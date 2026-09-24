struct Leaf(i32, i32);
struct Root {
    leaves: [Leaf; 2],
    pair: (i32, i32),
}

fn selected(root: &mut Root, index: usize) {
    let field = &mut root.leaves[index].0;
    *field = 41;
    println!("{}", *field);
}

fn choose(pair: &(i32, i32), first: bool) -> i32 {
    let reference = if first { &pair.0 } else { &pair.1 };
    *reference
}

fn replace(pair: &mut (i32, i32)) {
    let previous = *pair;
    *pair = (previous.0 + 15, previous.1 + 23);
}

fn flip(value: &mut bool) {
    *value = !*value;
}

fn main() {
    let mut root = Root {
        pair: (5, 6),
        leaves: [Leaf(1, 2), Leaf(3, 4)],
    };
    selected(&mut root, 1);
    println!("{}", root.leaves[0].1);
    println!("{}", root.leaves[1].0);
    println!("{}", choose(&root.pair, false));
    replace(&mut root.pair);
    println!("{}", root.pair.1);

    let first = 7;
    let second = 9;
    let condition = false;
    let reference = if condition { &first } else { &second };
    println!("{}", *reference);

    let values = [11, 13, 17];
    let slice: &[i32] = &values;
    let offset: usize = 1;
    println!("{}", slice[offset]);
    let mut enabled = false;
    flip(&mut enabled);
    println!("{}", enabled);
}

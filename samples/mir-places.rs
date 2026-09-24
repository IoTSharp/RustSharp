struct Leaf(i32, i32);
struct Root {
    leaves: [Leaf; 2],
    pair: (i32, i32),
}

fn selected(root: &mut Root, index: usize) -> &mut i32 {
    &mut root.leaves[index].0
}

fn choose(pair: &(i32, i32), first: bool) -> &i32 {
    if first { &pair.0 } else { &pair.1 }
}

fn replace(pair: &mut (i32, i32)) {
    *pair = (20, 29);
}

fn main() {
    let mut root = Root {
        pair: (5, 6),
        leaves: [Leaf(1, 2), Leaf(3, 4)],
    };
    let field = selected(&mut root, 1);
    *field = 41;
    println!("{}", *field);
    println!("{}", root.leaves[0].1);
    println!("{}", root.leaves[1].0);
    let picked = choose(&root.pair, false);
    println!("{}", *picked);
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
}

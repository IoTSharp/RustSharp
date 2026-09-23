fn apply() -> i32 {
    let mut base = 2;
    let add = |value: i32| value + base;
    add(1)
}

fn main() {
    println!("{}", apply());
}

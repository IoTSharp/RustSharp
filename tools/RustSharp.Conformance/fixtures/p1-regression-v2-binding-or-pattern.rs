fn choose(value: (i32, i32)) -> i32 {
    match value {
        (x, 0) | (0, x) => x,
        _ => -1,
    }
}

fn main() {
    println!("{}", choose((0, 7)));
}
